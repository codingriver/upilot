using System;
using System.IO;
using System.Linq;
using CodingRiver.UPilot.Automation;
using NUnit.Framework;
using UnityEngine;

namespace CodingRiver.UPilot.Tests.Automation
{
    public class AutomationReportWriterTests
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
            AutomationReportWriter writer = Create("run-a", 100);
            writer.Append(new AutomationReportEvent { eventType = "phase.started", phaseId = "prepare", timestampUtcMs = 110 });
            var summary = Summary("Passed", 200);
            summary.artifacts = new[]
            {
                new AutomationReportArtifactReference { kind = "console", path = "Log/UPilotConsole/raw/console.jsonl", external = true },
            };

            AutomationReportSummary completed = writer.Complete(summary);
            AutomationReportSummary repeated = writer.Complete(summary);
            AutomationReportArtifact[] artifacts = writer.GetArtifacts();

            Assert.That(completed.finishedAtUtcMs, Is.EqualTo(200));
            Assert.That(repeated.outcome, Is.EqualTo("Passed"));
            Assert.That(artifacts.Select(item => item.kind), Is.EquivalentTo(new[] { "events", "summary", "report", "timing" }));
            Assert.That(artifacts.All(item => item.bytes > 0 && item.sha256.Length == 64), Is.True);
            Assert.That(File.Exists(Path.Combine(writer.DirectoryPath, "Log", "UPilotConsole", "raw", "console.jsonl")), Is.False);
            Assert.Throws<InvalidOperationException>(() => writer.Append(new AutomationReportEvent { eventType = "late" }));
        }

        [Test]
        public void CompletionCannotRewriteOutcomeOrTerminalTime()
        {
            AutomationReportWriter writer = Create("run-b", 100);
            writer.Complete(Summary("Failed", 200));

            Assert.Throws<InvalidOperationException>(() => writer.Complete(Summary("Passed", 200)));
            Assert.Throws<InvalidOperationException>(() => writer.Complete(Summary("Failed", 201)));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void LegacyCompletionIsIdempotentWithoutBackfillingExportsOrChangingBytes(bool withBom)
        {
            var writer = Create("legacy", 100);
            string path = Path.Combine(writer.DirectoryPath, "summary.json");
            const string legacy = "{\"version\":1,\"runId\":\"legacy\",\"startedAtUtcMs\":100,\"finishedAtUtcMs\":200,\"outcome\":\"Failed\"}";
            File.WriteAllText(path, legacy, new System.Text.UTF8Encoding(withBom));
            byte[] bytes = File.ReadAllBytes(path);
            Assert.That(bytes.Take(3).SequenceEqual(new byte[] { 0xef, 0xbb, 0xbf }), Is.EqualTo(withBom));
            long modified = File.GetLastWriteTimeUtc(path).Ticks;
            var reopened = AutomationReportWriter.OpenExisting(_directory, "legacy");
            var requested = new AutomationReportSummary { outcome = "Failed", finishedAtUtcMs = 200 };
            Assert.That(reopened.Complete(requested).exportVersion, Is.Zero);
            Assert.That(reopened.Complete(requested).outcome, Is.EqualTo("Failed"));
            Assert.That(reopened.GetArtifacts().Select(a => a.kind), Is.EquivalentTo(new[] { "events", "summary" }));
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(bytes));
            Assert.That(File.GetLastWriteTimeUtc(path).Ticks, Is.EqualTo(modified));
            Assert.That(File.Exists(Path.Combine(writer.DirectoryPath, "report.txt")), Is.False);
            Assert.That(File.Exists(Path.Combine(writer.DirectoryPath, "timing.csv")), Is.False);
            requested.outcome = "Succeeded";
            Assert.Throws<InvalidOperationException>(() => reopened.Complete(requested));
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void SummaryPreambleChangesAfterOpenAreRejected(bool legacy, bool withBom)
        {
            var writer = Create("preamble", 100);
            var requested = Summary("Failed", 200);
            string path = Path.Combine(writer.DirectoryPath, "summary.json");
            string json;
            if (legacy)
            {
                json = "{\"version\":1,\"runId\":\"preamble\",\"startedAtUtcMs\":100,\"finishedAtUtcMs\":200,\"outcome\":\"Failed\"}";
                requested = new AutomationReportSummary { outcome = "Failed", finishedAtUtcMs = 200 };
            }
            else
            {
                writer.Complete(requested);
                json = File.ReadAllText(path);
            }
            File.WriteAllText(path, json, new System.Text.UTF8Encoding(withBom));
            var reopened = AutomationReportWriter.OpenExisting(_directory, "preamble");
            reopened.Complete(requested);
            Assert.That(reopened.GetArtifacts().Single(a => a.kind == "summary").bytes,
                Is.EqualTo(new FileInfo(path).Length));

            File.WriteAllText(path, json, new System.Text.UTF8Encoding(!withBom));
            byte[] changedBytes = File.ReadAllBytes(path);
            Assert.Throws<InvalidDataException>(() => reopened.GetArtifacts());
            Assert.Throws<InvalidDataException>(() => reopened.Complete(requested));
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(changedBytes));
        }

        [Test]
        public void OpenExistingValidatesIdentityAndCorruptTail()
        {
            AutomationReportWriter writer = Create("run-c", 100);
            writer.Append(new AutomationReportEvent { eventType = "case.started", caseId = "case-a", timestampUtcMs = 120 });

            AutomationReportWriter reopened = AutomationReportWriter.OpenExisting(_directory, "run-c");
            reopened.Append(new AutomationReportEvent { eventType = "case.finished", caseId = "case-a", timestampUtcMs = 130 });
            Assert.Throws<InvalidDataException>(() => AutomationReportWriter.OpenExisting(_directory, "wrong-run"));

            File.AppendAllText(Path.Combine(writer.DirectoryPath, "events.jsonl"), "not-json\n");
            Assert.Throws<InvalidDataException>(() => AutomationReportWriter.OpenExisting(_directory, "run-c"));
        }

        [Test]
        public void OutputMustRemainInsideCurrentProject()
        {
            string outside = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "outside-" + Guid.NewGuid().ToString("N")));
            Assert.Throws<InvalidOperationException>(() => AutomationReportWriter.Create(new AutomationReportCreateRequest
            {
                outputDirectory = outside,
                runId = "outside",
            }));
        }

        [Test]
        public void ArtifactMetadataRejectsMissingDirectoryOutsideAndConcurrentWriter()
        {
            var writer = Create("metadata", 100);
            string path = Path.Combine(writer.DirectoryPath, "attachment.json");
            Assert.Throws<FileNotFoundException>(() => AutomationReportWriter.GetArtifactMetadata("file", path));
            Assert.Throws<IOException>(() => AutomationReportWriter.GetArtifactMetadata("file", writer.DirectoryPath));
            Assert.Throws<InvalidOperationException>(() =>
                AutomationReportWriter.GetArtifactMetadata("file", Path.Combine(Application.dataPath, "../../outside")));
            File.WriteAllText(path, "{}");
            var original = AutomationReportWriter.GetArtifactMetadata("file", path);
            Assert.That(original.bytes, Is.EqualTo(2));
            Assert.That(original.sha256.Length, Is.EqualTo(64));
            using (new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
                Assert.Throws<IOException>(() => AutomationReportWriter.GetArtifactMetadata("file", path));
        }

        [Test]
        public void ConsolePolicySummaryIsBoundedAndFullClassificationIsImmutable()
        {
            var writer = Create("policy-run", 100);
            string longMessage = new string('x', 2047) + "\ud83d\ude00" + new string('y', 3000);
            var records = Enumerable.Range(10, 15).Select(i => new ConsoleCaptureRecord
            { sequence = i, logType = "Error", message = longMessage, stackTrace = "full stack" }).ToArray();
            var policy = AutomationLogPolicy.Evaluate(records, new[]
            {
                new AutomationLogInterval { phaseId = "Normal", caseId = "normal", fromSequenceInclusive = 10, toSequenceExclusive = 11 },
                new AutomationLogInterval { phaseId = "Finally", caseId = "exit", fromSequenceInclusive = 11, toSequenceExclusive = 25 },
            }, new[] { new AutomationLogRule { id = "deny", action = "deny", messageContains = "xxx" } });
            var summary = writer.WriteConsolePolicy("capture", 10, 25, policy, 0);
            Assert.That(summary.blockedCount, Is.EqualTo(15));
            Assert.That(summary.blockingSamples.Length, Is.EqualTo(10));
            Assert.That(summary.omittedBlockingCount, Is.EqualTo(5));
            Assert.That(summary.blockingSamples.All(s => s.message.Length == 2047 && s.textTruncated && s.hasStackTrace), Is.True);
            Assert.That(summary.blockingSamples[0].sequence, Is.EqualTo(10));
            Assert.That(summary.blockingSamples[0].instanceId, Is.EqualTo("normal"));
            Assert.That(summary.blockingSamples[1].phaseId, Is.EqualTo("Finally"));
            Assert.That(summary.blockingSamples[1].ruleId, Is.EqualTo("deny"));
            var artifact = writer.GetArtifacts().Single(a => a.kind == "consolePolicy");
            Assert.That(artifact.sha256.Length, Is.EqualTo(64));
            string full = File.ReadAllText(artifact.path);
            var parsed = AutomationStepJsonCodec.ParseObject(full);
            Assert.That(parsed.Element("runId").Value, Is.EqualTo("policy-run"));
            Assert.That(parsed.Element("policy").Element("classifications").Elements().Count(), Is.EqualTo(15));
            Assert.That(full, Does.Contain(longMessage));
            long modified = File.GetLastWriteTimeUtc(artifact.path).Ticks;
            var reopened = AutomationReportWriter.OpenExisting(_directory, "policy-run");
            reopened.WriteConsolePolicy("capture", 10, 25, policy, 0);
            Assert.That(File.GetLastWriteTimeUtc(artifact.path).Ticks, Is.EqualTo(modified));
            Assert.Throws<InvalidDataException>(() => reopened.WriteConsolePolicy("other", 10, 25, policy, 0));
            var terminal = Summary("Failed", 200);
            terminal.logSummary = summary;
            reopened.Complete(terminal);
            Assert.Throws<InvalidOperationException>(() => reopened.WriteConsolePolicy("capture", 10, 25, policy, 0));
            Assert.That(reopened.GetArtifacts().Single(a => a.kind == "consolePolicy").sha256, Is.EqualTo(artifact.sha256));
        }

        [Test]
        public void ConsolePolicyIncompleteEvidenceAndDiagnosticsRemainExplicit()
        {
            var writer = Create("incomplete", 100);
            var rules = Enumerable.Range(0, 15).Select(i => new AutomationLogRule { id = new string('x', 400), action = "invalid" });
            var policy = AutomationLogPolicy.Evaluate(null, null, rules, new AutomationLogEvidence
            { pagesComplete = false, lostRecordCount = 2, detail = new string('d', 4000) });
            var summary = writer.WriteConsolePolicy("capture", 0, 0, policy, 2);
            Assert.That(summary.blockingSamples, Is.Empty);
            Assert.That(summary.omittedBlockingCount, Is.Zero);
            Assert.That(summary.passed, Is.False);
            Assert.That(summary.policyValid, Is.False);
            Assert.That(summary.evidenceComplete, Is.False);
            Assert.That(summary.lostRecordCount, Is.EqualTo(2));
            Assert.That(summary.diagnostics.Length, Is.EqualTo(10));
            Assert.That(summary.omittedDiagnosticCount, Is.EqualTo(policy.diagnostics.Count - 10));
            Assert.That(summary.diagnosticsTextTruncated, Is.True);
            Assert.That(summary.diagnostics.All(d => d.subjectId.Length <= 256 && d.message.Length <= 2048), Is.True);
            Assert.That(File.ReadAllText(writer.GetArtifacts().Single(a => a.kind == "consolePolicy").path),
                Does.Contain("LOG_EVIDENCE_INCOMPLETE"));
        }

        [Test]
        public void ExportsUseFinalOutcomeAndSeparateExecutionFromCleanup()
        {
            var writer = Create("exports", 1000);
            var summary = Summary("Failed", 5000);
            summary.failureSignature = "STEP_CONSOLE_POLICY_FAILED";
            summary.logSummary = new AutomationReportLogSummary { sessionId = "capture", blockedCount = 1, evidenceComplete = true };
            summary.cases = new[]
            {
                new AutomationReportCase { id = "exit", stepId = "upilot.enter_edit_mode", phaseId = "Finally", stage = "Completed",
                    outcome = "Succeeded", startedAtUtcMs = 2000, cleanupStartedAtUtcMs = 3500, finishedAtUtcMs = 4000 },
                new AutomationReportCase { id = "unstarted", outcome = "Skipped", finishedAtUtcMs = 4500 }
            };
            writer.Complete(summary);
            var artifacts = writer.GetArtifacts();
            string report = File.ReadAllText(artifacts.Single(a => a.kind == "report").path);
            string csv = File.ReadAllText(artifacts.Single(a => a.kind == "timing").path);
            Assert.That(report, Does.Contain("Outcome: Failed\r\n"));
            Assert.That(report, Does.Contain("STEP_CONSOLE_POLICY_FAILED"));
            Assert.That(report, Does.Contain("Console session: capture\r\n"));
            Assert.That(report, Does.Contain("Console policy passed: false\r\n"));
            Assert.That(report, Does.Not.Contain("Not captured; not validated"));
            Assert.That(report, Does.Contain("Finally | Succeeded"));
            Assert.That(csv, Does.Contain("\"1.500\",\"0.500\",\"2.000\""));
            Assert.That(csv, Does.Contain("\"unstarted\",\"\",\"\",\"\",\"Skipped\",\"\",\"\",\"4500\",\"\",\"\",\"\",\"\""));
            Assert.That(csv.Replace("\r\n", ""), Does.Not.Contain("\n"));
        }

        [Test]
        public void ExportsAreInvariantEscapedAndNotPublishedBeforeCommit()
        {
            var writer = Create("escaping", 100);
            Assert.That(writer.GetArtifacts().Select(a => a.kind), Is.EquivalentTo(new[] { "events" }));
            var summary = Summary("Succeeded", 2100);
            summary.logSummary = null;
            summary.cases = new[]
            {
                new AutomationReportCase { id = "=HYPERLINK(\"a,b\")", detail = "line1\nline2",
                    startedAtUtcMs = 100, finishedAtUtcMs = 2100, outcome = "Succeeded" }
            };
            var original = System.Globalization.CultureInfo.CurrentCulture;
            try
            {
                System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("fr-FR");
                writer.Complete(summary);
            }
            finally { System.Globalization.CultureInfo.CurrentCulture = original; }
            string csv = File.ReadAllText(writer.GetArtifacts().Single(a => a.kind == "timing").path);
            Assert.That(csv, Does.Contain("\"'=HYPERLINK(\"\"a,b\"\")\""));
            Assert.That(csv, Does.Contain("\"2.000\""));
            string report = File.ReadAllText(writer.GetArtifacts().Single(a => a.kind == "report").path);
            Assert.That(report, Does.Contain("line1\\nline2"));
            Assert.That(report, Does.Contain("Not captured; not validated"));
            Assert.That(report, Does.Not.Contain("Console policy passed:"));
            var before = writer.GetArtifacts().ToDictionary(a => a.kind, a => a.sha256);
            var reopened = AutomationReportWriter.OpenExisting(_directory, "escaping");
            reopened.Complete(summary);
            Assert.That(reopened.GetArtifacts().All(a => before[a.kind] == a.sha256), Is.True);
            Assert.That(File.ReadAllText(reopened.GetArtifacts().Single(a => a.kind == "report").path), Is.EqualTo(report));
        }

        [Test]
        public void ExportFailureDoesNotCommitAndIdenticalPartialExportCanResume()
        {
            var writer = Create("partial-exports", 100);
            string timing = Path.Combine(writer.DirectoryPath, "timing.csv");
            Directory.CreateDirectory(timing);
            Assert.Throws<IOException>(() => writer.Complete(Summary("Failed", 200)));
            Assert.That(writer.IsComplete, Is.False);
            Assert.That(File.Exists(Path.Combine(writer.DirectoryPath, "summary.json")), Is.False);
            Assert.That(writer.GetArtifacts().Select(a => a.kind), Is.EquivalentTo(new[] { "events" }));
            long modified = File.GetLastWriteTimeUtc(Path.Combine(writer.DirectoryPath, "report.txt")).Ticks;
            Directory.Delete(timing);
            AutomationReportWriter.OpenExisting(_directory, "partial-exports").Complete(Summary("Failed", 200));
            Assert.That(File.GetLastWriteTimeUtc(Path.Combine(writer.DirectoryPath, "report.txt")).Ticks, Is.EqualTo(modified));
        }

        [TestCase("report.txt")]
        [TestCase("timing.csv")]
        [TestCase("summary.json")]
        public void CompletedExportsAreImmutableAndTamperingIsNotRepaired(string fileName)
        {
            var writer = Create("immutable-exports", 100);
            writer.Complete(Summary("Failed", 200));
            string path = Path.Combine(writer.DirectoryPath, fileName);
            long modified = File.GetLastWriteTimeUtc(path).Ticks;
            var reopened = AutomationReportWriter.OpenExisting(_directory, "immutable-exports");
            reopened.Complete(Summary("Failed", 200));
            Assert.That(File.GetLastWriteTimeUtc(path).Ticks, Is.EqualTo(modified));
            File.WriteAllText(path, "tampered");
            Assert.Throws<InvalidDataException>(() => reopened.GetArtifacts());
            Assert.Throws<InvalidDataException>(() => reopened.Complete(Summary("Failed", 200)));
            Assert.That(File.ReadAllText(path), Is.EqualTo("tampered"));
        }

        [TestCase("report.txt")]
        [TestCase("timing.csv")]
        [TestCase("summary.json")]
        public void MissingCommittedFilePreventsPublishingOrRecreatingExports(string fileName)
        {
            var writer = Create("missing-export", 100);
            writer.Complete(Summary("Failed", 200));
            string path = Path.Combine(writer.DirectoryPath, fileName);
            File.Delete(path);
            Assert.Catch<IOException>(() => writer.GetArtifacts());
            Assert.Catch<IOException>(() => writer.Complete(Summary("Failed", 200)));
            Assert.That(File.Exists(path), Is.False);
        }

        private AutomationReportWriter Create(string runId, long startedAt) => AutomationReportWriter.Create(new AutomationReportCreateRequest
        {
            outputDirectory = _directory,
            runId = runId,
            startedAtUtcMs = startedAt,
        });

        private static AutomationReportSummary Summary(string outcome, long finishedAt) => new()
        {
            outcome = outcome,
            finishedAtUtcMs = finishedAt,
            detail = "terminal",
            logSummary = new AutomationReportLogSummary { evidenceComplete = true },
        };
    }
}
