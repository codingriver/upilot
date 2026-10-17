using System;
using System.Linq;
using NUnit.Framework;

namespace CodingRiver.UPilot.Tests
{
    public sealed class UPilotRestartObservationPolicyTests
    {
        private static UPilotServerRestartRecord Ready(int schema) => new UPilotServerRestartRecord {
            schemaVersion = schema, status = "running", requestedAtUtcMs = 100,
            deadlineAtUtcMs = 10000, projectIdentityVerified = true, bridgeVerified = true, readOnlyVerified = true,
            gateDiagnostics = new[] { "old_identity", "bridge_stop", "old_exit", "ports", "new_process", "identity", "bridge_session", "readonly" }
                .Select(key => new UPilotRestartGate { key = key, state = "passed" }).ToArray(),
        };

        [Test]
        public void V5CompletesWithoutHealthOrDeploymentButLegacyDoesNot()
        {
            var current = Ready(5);
            UPilotServerRestartDiagnostics.TryComplete(current, 200);
            Assert.That(current.status, Is.EqualTo("succeeded"));
            Assert.That(current.healthVerified, Is.False);
            Assert.That(current.deploymentVerified, Is.False);
            var legacy = Ready(4);
            UPilotServerRestartDiagnostics.TryComplete(legacy, 200);
            Assert.That(legacy.status, Is.EqualTo("running"));
            Assert.That(UPilotServerRestartDiagnostics.GateKeysFor(legacy),
                Does.Contain("health").And.Contain("deployment"));
        }

        [TestCase("old_identity")]
        [TestCase("old_exit")]
        [TestCase("ports")]
        [TestCase("new_process")]
        [TestCase("identity")]
        [TestCase("bridge_session")]
        [TestCase("readonly")]
        public void MissingNecessaryEvidenceCannotComplete(string missing)
        {
            var record = Ready(5);
            record.gateDiagnostics = record.gateDiagnostics.Where(g => g.key != missing).ToArray();
            UPilotServerRestartDiagnostics.TryComplete(record, 200);
            Assert.That(record.status, Is.EqualTo("running"));
        }

        [Test]
        public void MigrationPreservesOriginalOperationDeadlineAndActionReceiptsButDiscardsReadyEvidence()
        {
            var record = Ready(4);
            record.operationId = "original";
            record.restartGeneration = 7;
            record.resetReceipt = new UPilotRestartResetReceipt { operationId = "original", performed = true };
            var receipt = record.resetReceipt;
            UPilotServerRestartDiagnostics.MigrateVerifiedInFlight(record);
            Assert.That(record.schemaVersion, Is.EqualTo(5));
            Assert.That(record.operationId, Is.EqualTo("original"));
            Assert.That(record.deadlineAtUtcMs, Is.EqualTo(10000));
            Assert.That(record.restartGeneration, Is.EqualTo(7));
            Assert.That(record.resetReceipt, Is.SameAs(receipt));
            Assert.That(record.readOnlyVerified || record.bridgeVerified || record.projectIdentityVerified, Is.False);
            Assert.That(record.gateDiagnostics.Select(g => g.key), Does.Contain("old_exit").And.Not.Contain("readonly"));
        }

        [Test]
        public void TerminalLegacyAndUnknownFutureCannotBeReinterpreted()
        {
            var legacy = Ready(4); legacy.status = "failed";
            UPilotServerRestartDiagnostics.MigrateVerifiedInFlight(legacy);
            Assert.That(legacy.schemaVersion, Is.EqualTo(4));
            Assert.That(legacy.status, Is.EqualTo("failed"));
            var future = Ready(6);
            UPilotServerRestartDiagnostics.TryComplete(future, 200);
            Assert.That(future.status, Is.EqualTo("running"));
        }

        [TestCase("version")]
        [TestCase("channel")]
        [TestCase("configured_entry")]
        [TestCase("module")]
        [TestCase("health")]
        [TestCase("timeout")]
        public void AuxiliaryDeploymentObservationsNeverBlock(string code)
        {
            Assert.That(new UPilotDeploymentIssue { Code = code, Confirmed = true }.Blocking, Is.False);
        }

        [TestCase("authentication")]
        [TestCase("ownership")]
        [TestCase("project")]
        [TestCase("entry")]
        [TestCase("protocol")]
        [TestCase("instance")]
        public void RealIdentityAndProtocolFailuresRemainBlocking(string code)
        {
            Assert.That(new UPilotDeploymentIssue { Code = code, Confirmed = true }.Blocking, Is.True);
        }
    }
}
