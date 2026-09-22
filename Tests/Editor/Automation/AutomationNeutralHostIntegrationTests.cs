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
            AutomationCatalogDescriptorV1 catalog = Catalog("inspect.manifest", "inspect.package");
            AutomationSelectionResultV1 selection = AutomationSelectionV1.Analyze(catalog, new AutomationSelectionRequestV1
            {
                caseIds = new[] { "inspect.manifest", "inspect.package" },
            });
            Assert.That(selection.ok, Is.True);

            AutomationReportWriterV1 report = AutomationReportWriterV1.Create(new AutomationReportCreateRequestV1
            {
                outputDirectory = _reportDirectory,
                runId = "asset-inspection-host",
            });
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            foreach (string caseId in selection.selectedCaseIds)
            {
                report.Append(new AutomationReportEventV1 { eventType = "case.started", caseId = caseId });
                string target = caseId == "inspect.manifest"
                    ? Path.Combine(projectRoot, "Packages", "manifest.json")
                    : Path.Combine(projectRoot, "Packages", "packages-lock.json");
                Assert.That(File.Exists(target), Is.True, target);
                report.Append(new AutomationReportEventV1 { eventType = "case.finished", caseId = caseId, outcome = "Passed" });
            }

            AutomationLogPolicyResultV1 logs = AutomationLogPolicyV1.Evaluate(Array.Empty<ConsoleCaptureRecord>(), null, null);
            report.Complete(new AutomationReportSummaryV1
            {
                outcome = logs.passed ? "Passed" : "Failed",
                logSummary = new AutomationReportLogSummaryV1 { evidenceComplete = logs.evidenceComplete },
                cases = selection.selectedCaseIds.Select(id => new AutomationReportCaseV1 { id = id, outcome = "Passed" }).ToArray(),
            });
            Assert.That(report.GetArtifacts().Length, Is.EqualTo(2));
        }

        [UnityTest]
        public IEnumerator FrameDrivenCounterHostOwnsItsLoop()
        {
            AutomationSelectionResultV1 selection = AutomationSelectionV1.Analyze(
                Catalog("counter.frames"),
                new AutomationSelectionRequestV1 { caseIds = new[] { "counter.frames" } });
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

        private static AutomationCatalogDescriptorV1 Catalog(params string[] ids) => new()
        {
            cases = ids.Select(id => new AutomationCaseDescriptorV1 { id = id }).ToArray(),
            suites = Array.Empty<AutomationSuiteDescriptorV1>(),
        };
    }
}
