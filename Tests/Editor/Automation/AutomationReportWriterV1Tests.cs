using System;
using System.IO;
using System.Linq;
using CodingRiver.UPilot.Automation;
using NUnit.Framework;
using UnityEngine;

namespace CodingRiver.UPilot.Tests.Automation
{
    public class AutomationReportWriterV1Tests
    {
        private string _directory;

        [SetUp]
        public void SetUp()
        {
            _directory = Path.Combine("Log", "AutomationReportTests", Guid.NewGuid().ToString("N"));
        }

        [TearDown]
        public void TearDown()
        {
            string full = Path.GetFullPath(Path.Combine(Application.dataPath, "..", _directory));
            if (Directory.Exists(full)) Directory.Delete(full, true);
        }

        [Test]
        public void CreateAppendCompleteAndArtifactsAreStable()
        {
            AutomationReportWriterV1 writer = Create("run-a", 100);
            writer.Append(new AutomationReportEventV1 { eventType = "phase.started", phaseId = "prepare", timestampUtcMs = 110 });
            var summary = Summary("Passed", 200);
            summary.artifacts = new[]
            {
                new AutomationReportArtifactReferenceV1 { kind = "console", path = "Log/UPilotConsole/raw/console.jsonl", external = true },
            };

            AutomationReportSummaryV1 completed = writer.Complete(summary);
            AutomationReportSummaryV1 repeated = writer.Complete(summary);
            AutomationReportArtifactV1[] artifacts = writer.GetArtifacts();

            Assert.That(completed.finishedAtUtcMs, Is.EqualTo(200));
            Assert.That(repeated.outcome, Is.EqualTo("Passed"));
            Assert.That(artifacts.Select(item => item.kind), Is.EquivalentTo(new[] { "events", "summary" }));
            Assert.That(artifacts.All(item => item.bytes > 0 && item.sha256.Length == 64), Is.True);
            Assert.That(File.Exists(Path.Combine(writer.DirectoryPath, "Log", "UPilotConsole", "raw", "console.jsonl")), Is.False);
            Assert.Throws<InvalidOperationException>(() => writer.Append(new AutomationReportEventV1 { eventType = "late" }));
        }

        [Test]
        public void CompletionCannotRewriteOutcomeOrTerminalTime()
        {
            AutomationReportWriterV1 writer = Create("run-b", 100);
            writer.Complete(Summary("Failed", 200));

            Assert.Throws<InvalidOperationException>(() => writer.Complete(Summary("Passed", 200)));
            Assert.Throws<InvalidOperationException>(() => writer.Complete(Summary("Failed", 201)));
        }

        [Test]
        public void OpenExistingValidatesIdentityAndCorruptTail()
        {
            AutomationReportWriterV1 writer = Create("run-c", 100);
            writer.Append(new AutomationReportEventV1 { eventType = "case.started", caseId = "case-a", timestampUtcMs = 120 });

            AutomationReportWriterV1 reopened = AutomationReportWriterV1.OpenExisting(_directory, "run-c");
            reopened.Append(new AutomationReportEventV1 { eventType = "case.finished", caseId = "case-a", timestampUtcMs = 130 });
            Assert.Throws<InvalidDataException>(() => AutomationReportWriterV1.OpenExisting(_directory, "wrong-run"));

            File.AppendAllText(Path.Combine(writer.DirectoryPath, "events.jsonl"), "not-json\n");
            Assert.Throws<InvalidDataException>(() => AutomationReportWriterV1.OpenExisting(_directory, "run-c"));
        }

        [Test]
        public void OutputMustRemainInsideCurrentProject()
        {
            string outside = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "outside-" + Guid.NewGuid().ToString("N")));
            Assert.Throws<InvalidOperationException>(() => AutomationReportWriterV1.Create(new AutomationReportCreateRequestV1
            {
                outputDirectory = outside,
                runId = "outside",
            }));
        }

        private AutomationReportWriterV1 Create(string runId, long startedAt) => AutomationReportWriterV1.Create(new AutomationReportCreateRequestV1
        {
            outputDirectory = _directory,
            runId = runId,
            startedAtUtcMs = startedAt,
        });

        private static AutomationReportSummaryV1 Summary(string outcome, long finishedAt) => new()
        {
            outcome = outcome,
            finishedAtUtcMs = finishedAt,
            detail = "terminal",
            logSummary = new AutomationReportLogSummaryV1 { evidenceComplete = true },
        };
    }
}
