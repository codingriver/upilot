using System;
using System.Collections;
using System.IO;
using System.Linq;
using CodingRiver.UPilot.Automation;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CodingRiver.UPilot.Tests.Automation
{
    public class AutomationNeutralHostIntegrationTests
    {
        private string _reportDirectory;

        [SetUp]
        public void SetUp()
        {
            _reportDirectory = Path.Combine("Log", "AutomationNeutralHostTests", Guid.NewGuid().ToString("N"));
        }

        [TearDown]
        public void TearDown()
        {
            string full = Path.GetFullPath(Path.Combine(Application.dataPath, "..", _reportDirectory));
            if (Directory.Exists(full)) Directory.Delete(full, true);
        }

        [Test]
        public void AssetInspectionHostOwnsItsExecutionAndUsesOnlySupportModules()
        {
            AutomationCatalogDescriptor catalog = Catalog("inspect.manifest", "inspect.package");
            AutomationSelectionResult selection = AutomationSelection.Analyze(catalog, new AutomationSelectionRequest
            {
                caseIds = new[] { "inspect.manifest", "inspect.package" },
            });
            Assert.That(selection.ok, Is.True);

            AutomationReportWriter report = AutomationReportWriter.Create(new AutomationReportCreateRequest
            {
                outputDirectory = _reportDirectory,
                runId = "asset-inspection-host",
            });
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            foreach (string caseId in selection.selectedCaseIds)
            {
                report.Append(new AutomationReportEvent { eventType = "case.started", caseId = caseId });
                string target = caseId == "inspect.manifest"
                    ? Path.Combine(projectRoot, "Packages", "manifest.json")
                    : Path.Combine(projectRoot, "Packages", "packages-lock.json");
                Assert.That(File.Exists(target), Is.True, target);
                report.Append(new AutomationReportEvent { eventType = "case.finished", caseId = caseId, outcome = "Passed" });
            }

            AutomationLogPolicyResult logs = AutomationLogPolicy.Evaluate(Array.Empty<ConsoleCaptureRecord>(), null, null);
            report.Complete(new AutomationReportSummary
            {
                outcome = logs.passed ? "Passed" : "Failed",
                logSummary = new AutomationReportLogSummary { evidenceComplete = logs.evidenceComplete },
                cases = selection.selectedCaseIds.Select(id => new AutomationReportCase { id = id, outcome = "Passed" }).ToArray(),
            });
            Assert.That(report.GetArtifacts().Select(a => a.kind),
                Is.EquivalentTo(new[] { "events", "summary", "report", "timing" }));
        }

        [UnityTest]
        public IEnumerator FrameDrivenCounterHostOwnsItsLoop()
        {
            AutomationSelectionResult selection = AutomationSelection.Analyze(
                Catalog("counter.frames"),
                new AutomationSelectionRequest { caseIds = new[] { "counter.frames" } });
            Assert.That(selection.ok, Is.True);

            int counter = 0;
            while (counter < 3)
            {
                counter++;
                yield return null;
            }

            Assert.That(counter, Is.EqualTo(3));
            Assert.That(selection.selectedCaseIds, Is.EqualTo(new[] { "counter.frames" }));
        }

        private static AutomationCatalogDescriptor Catalog(params string[] ids) => new()
        {
            cases = ids.Select(id => new AutomationCaseDescriptor { id = id }).ToArray(),
            suites = Array.Empty<AutomationSuiteDescriptor>(),
        };
    }
}
