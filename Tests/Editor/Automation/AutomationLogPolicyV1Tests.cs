using System;
using System.Linq;
using CodingRiver.UPilot.Automation;
using NUnit.Framework;

namespace CodingRiver.UPilot.Tests.Automation
{
    public class AutomationLogPolicyV1Tests
    {
        [Test]
        public void DefaultPolicyBlocksErrorExceptionAndAssert()
        {
            AutomationLogPolicyResultV1 result = AutomationLogPolicyV1.Evaluate(new[]
            {
                Record(0, "Log"), Record(1, "Warning"), Record(2, "Error"),
                Record(3, "Exception"), Record(4, "Assert"),
            }, null, null);

            Assert.That(result.evidenceComplete, Is.True);
            Assert.That(result.blockedCount, Is.EqualTo(3));
            Assert.That(result.ignoredCount, Is.EqualTo(2));
            Assert.That(result.passed, Is.False);
        }

        [Test]
        public void DenyWinsAndFirstAllowBudgetAppliesAcrossCases()
        {
            AutomationLogRuleV1[] rules =
            {
                Rule("allow-one", "allow", maximumCount: 1, contains: "known"),
                Rule("allow-all", "allow", maximumCount: -1, contains: "known"),
                Rule("deny-cleanup", "deny", maximumCount: -1, contains: "cleanup"),
            };
            AutomationLogPolicyResultV1 result = AutomationLogPolicyV1.Evaluate(new[]
            {
                Record(1, "Error", "known first"),
                Record(11, "Error", "known second"),
                Record(12, "Error", "known cleanup"),
            }, new[]
            {
                Interval(0, 10, "case-a"),
                Interval(10, 20, "case-b"),
            }, rules);

            Assert.That(result.classifications.Select(item => item.disposition),
                Is.EqualTo(new[] { "allowed", "blocked", "blocked" }));
            Assert.That(result.classifications[1].ruleId, Is.EqualTo("allow-one"));
            Assert.That(result.classifications[2].ruleId, Is.EqualTo("deny-cleanup"));
        }

        [Test]
        public void ScopedAllowCannotMatchUnknownContextAndHalfOpenRangesAreExact()
        {
            AutomationLogRuleV1 scoped = Rule("case-allow", "allow", -1, "expected");
            scoped.caseIds = new[] { "case-a" };
            AutomationLogPolicyResultV1 result = AutomationLogPolicyV1.Evaluate(new[]
            {
                Record(9, "Error", "expected"),
                Record(10, "Error", "expected"),
                Record(20, "Error", "expected"),
            }, new[]
            {
                Interval(10, 20, "case-a"),
            }, new[] { scoped });

            Assert.That(result.classifications.Select(item => item.disposition),
                Is.EqualTo(new[] { "blocked", "allowed", "blocked" }));
            Assert.That(result.classifications[0].context.known, Is.False);
            Assert.That(result.classifications[1].context.caseId, Is.EqualTo("case-a"));
            Assert.That(result.classifications[2].context.known, Is.False);
        }

        [Test]
        public void PhaseAndCaseIntervalsMayOverlapAndMergeContext()
        {
            AutomationLogRuleV1 scoped = Rule("scoped", "allow", -1, "expected");
            scoped.phaseIds = new[] { "cleanup" };
            scoped.caseIds = new[] { "case-a" };
            AutomationLogPolicyResultV1 result = AutomationLogPolicyV1.Evaluate(
                new[] { Record(5, "Error", "expected") },
                new[]
                {
                    new AutomationLogIntervalV1 { fromSequenceInclusive = 0, toSequenceExclusive = 10, phaseId = "cleanup" },
                    new AutomationLogIntervalV1 { fromSequenceInclusive = 2, toSequenceExclusive = 8, caseId = "case-a" },
                },
                new[] { scoped });

            Assert.That(result.policyValid, Is.True);
            Assert.That(result.passed, Is.True);
            Assert.That(result.classifications[0].context.phaseId, Is.EqualTo("cleanup"));
            Assert.That(result.classifications[0].context.caseId, Is.EqualTo("case-a"));
        }

        [Test]
        public void IncompleteEvidenceCanNeverPass()
        {
            AutomationLogPolicyResultV1 result = AutomationLogPolicyV1.Evaluate(
                new[] { Record(0, "Log") },
                null,
                null,
                new AutomationLogEvidenceV1
                {
                    expectedSessionId = "expected",
                    actualSessionId = "other",
                    lostRecordCount = 2,
                    pagesComplete = false,
                });

            Assert.That(result.blockedCount, Is.Zero);
            Assert.That(result.evidenceComplete, Is.False);
            Assert.That(result.passed, Is.False);
            Assert.That(result.diagnostics.Select(item => item.code), Does.Contain("LOG_EVIDENCE_INCOMPLETE"));
        }

        [Test]
        public void FingerprintsNormalizeGuidWhitespaceAndStackLinesButKeepBusinessIds()
        {
            ConsoleCaptureRecord first = Record(0, "Error", "unit 10001 failed  550e8400-e29b-41d4-a716-446655440000");
            first.stackTrace = "at Sample.Run() in C:/Sample.cs:line 42";
            ConsoleCaptureRecord equivalent = Record(1, "Error", "unit 10001 failed\n550e8400-e29b-41d4-a716-446655440999");
            equivalent.stackTrace = "at Sample.Run() in C:/Sample.cs:line 900";
            ConsoleCaptureRecord otherBusinessId = Record(2, "Error", "unit 10002 failed 550e8400-e29b-41d4-a716-446655440111");
            otherBusinessId.stackTrace = "at Sample.Run() in C:/Sample.cs:line 42";

            Assert.That(AutomationLogPolicyV1.Fingerprint(first), Is.EqualTo(AutomationLogPolicyV1.Fingerprint(equivalent)));
            Assert.That(AutomationLogPolicyV1.Fingerprint(first), Is.Not.EqualTo(AutomationLogPolicyV1.Fingerprint(otherBusinessId)));
        }

        private static ConsoleCaptureRecord Record(long sequence, string level, string message = "message") => new()
        {
            sequence = sequence,
            logType = level,
            message = message,
            stackTrace = string.Empty,
        };

        private static AutomationLogIntervalV1 Interval(long from, long to, string caseId) => new()
        {
            fromSequenceInclusive = from,
            toSequenceExclusive = to,
            caseId = caseId,
            phaseId = "cases",
        };

        private static AutomationLogRuleV1 Rule(string id, string action, int maximumCount, string contains) => new()
        {
            id = id,
            action = action,
            levels = new[] { "Error" },
            messageContains = contains,
            maximumCount = maximumCount,
        };
    }
}
