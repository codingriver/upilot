using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace CodingRiver.UPilot.Automation
{
    [Serializable]
    public sealed class AutomationLogInterval
    {
        public string phaseId;
        public string caseId;
        public long fromSequenceInclusive;
        public long toSequenceExclusive;
    }

    [Serializable]
    public sealed class AutomationLogRule
    {
        public string id;
        public string action = "allow";
        public string[] levels = Array.Empty<string>();
        public string messageContains;
        public string messageRegex;
        public string stackContains;
        public string stackRegex;
        public string[] phaseIds = Array.Empty<string>();
        public string[] caseIds = Array.Empty<string>();
        public int maximumCount = -1;
    }

    [Serializable]
    public sealed class AutomationLogEvidence
    {
        public string expectedSessionId;
        public string actualSessionId;
        public bool identityMatches = true;
        public bool pagesComplete = true;
        public bool readSucceeded = true;
        public bool writeSucceeded = true;
        public bool timedOut;
        public long lostRecordCount;
        public string detail;
    }

    [Serializable]
    public sealed class AutomationLogContext
    {
        public string phaseId;
        public string caseId;
        public bool known;
    }

    [Serializable]
    public sealed class AutomationLogClassification
    {
        public ConsoleCaptureRecord record;
        public AutomationLogContext context;
        public string disposition;
        public string ruleId;
        public string reason;
        public string fingerprint;
    }

    [Serializable]
    public sealed class AutomationLogFingerprintCount
    {
        public string fingerprint;
        public string level;
        public int count;
        public long firstSequence;
        public long lastSequence;
    }

    [Serializable]
    public sealed class AutomationLogPolicyResult
    {
        public bool ok;
        public bool passed;
        public bool policyValid;
        public bool evidenceComplete;
        public int totalCount;
        public int allowedCount;
        public int blockedCount;
        public int ignoredCount;
        public List<AutomationDiagnostic> diagnostics = new();
        public List<AutomationLogClassification> classifications = new();
        public List<AutomationLogFingerprintCount> fingerprints = new();
    }

    public static class AutomationLogPolicy
    {
        private static readonly Regex GuidPattern = new(
            @"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b",
            RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100));
        private static readonly Regex StackLinePattern = new(
            @"(?<prefix>(?:\.cs:|\bline\s+))\d+\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100));
        private static readonly Regex WhitespacePattern = new(
            @"\s+",
            RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100));

        private sealed class PreparedRule
        {
            public AutomationLogRule Rule;
            public Regex MessageRegex;
            public Regex StackRegex;
            public int MatchCount;
        }

        public static AutomationLogPolicyResult Evaluate(
            IEnumerable<ConsoleCaptureRecord> records,
            IEnumerable<AutomationLogInterval> intervals,
            IEnumerable<AutomationLogRule> rules,
            AutomationLogEvidence evidence = null)
        {
            var result = new AutomationLogPolicyResult { policyValid = true };
            List<AutomationLogInterval> intervalList = (intervals ?? Array.Empty<AutomationLogInterval>()).ToList();
            ValidateIntervals(intervalList, result);
            List<PreparedRule> prepared = PrepareRules(rules, result);
            EvaluateEvidence(evidence ?? new AutomationLogEvidence(), result);

            var denyRules = prepared.Where(item => IsAction(item.Rule, "deny")).ToList();
            var allowRules = prepared.Where(item => IsAction(item.Rule, "allow")).ToList();
            var fingerprintCounts = new Dictionary<string, AutomationLogFingerprintCount>(StringComparer.Ordinal);
            foreach (ConsoleCaptureRecord record in (records ?? Array.Empty<ConsoleCaptureRecord>()).Where(item => item != null))
            {
                AutomationLogContext context = ResolveContext(record.sequence, intervalList, result);
                string fingerprint = Fingerprint(record);
                var classification = new AutomationLogClassification
                {
                    record = record,
                    context = context,
                    fingerprint = fingerprint,
                    disposition = "ignored",
                    reason = "Level is not blocking by default.",
                };

                PreparedRule deny = denyRules.FirstOrDefault(item => Matches(item, record, context));
                if (deny != null)
                {
                    deny.MatchCount++;
                    classification.disposition = "blocked";
                    classification.ruleId = deny.Rule.id;
                    classification.reason = "Matched deny rule.";
                }
                else
                {
                    PreparedRule allow = allowRules.FirstOrDefault(item => Matches(item, record, context));
                    if (allow != null)
                    {
                        allow.MatchCount++;
                        classification.ruleId = allow.Rule.id;
                        if (allow.Rule.maximumCount >= 0 && allow.MatchCount > allow.Rule.maximumCount)
                        {
                            classification.disposition = "blocked";
                            classification.reason = "Allow rule budget exceeded.";
                        }
                        else
                        {
                            classification.disposition = "allowed";
                            classification.reason = "Matched allow rule.";
                        }
                    }
                    else if (IsDefaultBlockingLevel(record.logType))
                    {
                        classification.disposition = "blocked";
                        classification.reason = "Error, Exception, and Assert block by default.";
                    }
                }

                result.classifications.Add(classification);
                AddFingerprint(fingerprintCounts, record, fingerprint);
            }

            result.totalCount = result.classifications.Count;
            result.allowedCount = result.classifications.Count(item => item.disposition == "allowed");
            result.blockedCount = result.classifications.Count(item => item.disposition == "blocked");
            result.ignoredCount = result.classifications.Count(item => item.disposition == "ignored");
            result.fingerprints = fingerprintCounts.Values.OrderBy(item => item.firstSequence).ToList();
            result.ok = result.policyValid;
            result.passed = result.policyValid && result.evidenceComplete && result.blockedCount == 0;
            return result;
        }

        public static string Fingerprint(ConsoleCaptureRecord record)
        {
            if (record == null)
                return string.Empty;
            string source = (record.logType ?? string.Empty) + "\n"
                + (record.message ?? string.Empty) + "\n"
                + (record.stackTrace ?? string.Empty);
            source = GuidPattern.Replace(source, "{guid}");
            source = StackLinePattern.Replace(source, match => match.Groups["prefix"].Value + "{line}");
            source = WhitespacePattern.Replace(source, " ").Trim();
            using var sha = SHA256.Create();
            return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(source)).Select(value => value.ToString("x2")));
        }

        private static void ValidateIntervals(List<AutomationLogInterval> intervals, AutomationLogPolicyResult result)
        {
            foreach (AutomationLogInterval interval in intervals)
            {
                if (interval == null || interval.fromSequenceInclusive < 0
                    || interval.toSequenceExclusive <= interval.fromSequenceInclusive)
                {
                    result.policyValid = false;
                    AutomationCatalog.Add(result.diagnostics, "LOG_INTERVAL_INVALID", "Log intervals must be non-empty half-open sequence ranges.");
                }
            }
        }

        private static List<PreparedRule> PrepareRules(
            IEnumerable<AutomationLogRule> rules,
            AutomationLogPolicyResult result)
        {
            var prepared = new List<PreparedRule>();
            var ids = new HashSet<string>(AutomationCatalog.IdComparer);
            int index = 0;
            foreach (AutomationLogRule rule in rules ?? Array.Empty<AutomationLogRule>())
            {
                string id = AutomationCatalog.NormalizeId(rule?.id);
                if (rule == null || string.IsNullOrEmpty(id) || !ids.Add(id)
                    || (!IsAction(rule, "allow") && !IsAction(rule, "deny")) || rule.maximumCount < -1)
                {
                    result.policyValid = false;
                    AutomationCatalog.Add(result.diagnostics, "LOG_RULE_INVALID", "Rule ID/action/budget is invalid.", id, index++);
                    continue;
                }

                try
                {
                    prepared.Add(new PreparedRule
                    {
                        Rule = rule,
                        MessageRegex = Compile(rule.messageRegex),
                        StackRegex = Compile(rule.stackRegex),
                    });
                }
                catch (ArgumentException ex)
                {
                    result.policyValid = false;
                    AutomationCatalog.Add(result.diagnostics, "LOG_RULE_REGEX_INVALID", ex.Message, id, index);
                }
                index++;
            }
            return prepared;
        }

        private static Regex Compile(string pattern) => string.IsNullOrWhiteSpace(pattern)
            ? null
            : new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));

        private static void EvaluateEvidence(AutomationLogEvidence evidence, AutomationLogPolicyResult result)
        {
            bool explicitIdentityMatches = evidence.identityMatches
                && (string.IsNullOrEmpty(evidence.expectedSessionId)
                    || string.Equals(evidence.expectedSessionId, evidence.actualSessionId, StringComparison.Ordinal));
            result.evidenceComplete = explicitIdentityMatches
                && evidence.pagesComplete
                && evidence.readSucceeded
                && evidence.writeSucceeded
                && !evidence.timedOut
                && evidence.lostRecordCount == 0;
            if (!result.evidenceComplete)
                AutomationCatalog.Add(result.diagnostics, "LOG_EVIDENCE_INCOMPLETE", "Console evidence is incomplete: " + (evidence.detail ?? string.Empty));
        }

        private static AutomationLogContext ResolveContext(
            long sequence,
            List<AutomationLogInterval> intervals,
            AutomationLogPolicyResult result)
        {
            List<AutomationLogInterval> matches = intervals
                .Where(item => item != null
                    && sequence >= item.fromSequenceInclusive
                    && sequence < item.toSequenceExclusive)
                .ToList();
            string[] phases = matches.Select(item => item.phaseId)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(AutomationCatalog.IdComparer)
                .ToArray();
            string[] cases = matches.Select(item => item.caseId)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(AutomationCatalog.IdComparer)
                .ToArray();
            if (phases.Length > 1 || cases.Length > 1)
            {
                result.policyValid = false;
                AutomationCatalog.Add(result.diagnostics, "LOG_CONTEXT_CONFLICT", "A Console sequence belongs to conflicting phase or Case contexts.", sequence.ToString());
            }
            return new AutomationLogContext
            {
                known = matches.Count > 0,
                phaseId = phases.FirstOrDefault() ?? string.Empty,
                caseId = cases.FirstOrDefault() ?? string.Empty,
            };
        }

        private static bool Matches(PreparedRule prepared, ConsoleCaptureRecord record, AutomationLogContext context)
        {
            AutomationLogRule rule = prepared.Rule;
            if (!MatchesAny(rule.levels, record.logType)) return false;
            if (!string.IsNullOrEmpty(rule.messageContains)
                && (record.message ?? string.Empty).IndexOf(rule.messageContains, StringComparison.Ordinal) < 0) return false;
            if (prepared.MessageRegex != null && !prepared.MessageRegex.IsMatch(record.message ?? string.Empty)) return false;
            if (!string.IsNullOrEmpty(rule.stackContains)
                && (record.stackTrace ?? string.Empty).IndexOf(rule.stackContains, StringComparison.Ordinal) < 0) return false;
            if (prepared.StackRegex != null && !prepared.StackRegex.IsMatch(record.stackTrace ?? string.Empty)) return false;

            bool phaseScoped = rule.phaseIds != null && rule.phaseIds.Length > 0;
            bool caseScoped = rule.caseIds != null && rule.caseIds.Length > 0;
            if ((phaseScoped || caseScoped) && !context.known) return false;
            if (phaseScoped && !MatchesAny(rule.phaseIds, context.phaseId)) return false;
            if (caseScoped && !MatchesAny(rule.caseIds, context.caseId)) return false;
            return true;
        }

        private static bool MatchesAny(string[] values, string actual) => values == null || values.Length == 0
            || values.Any(value => AutomationCatalog.IdComparer.Equals(value?.Trim(), actual?.Trim()));

        private static bool IsAction(AutomationLogRule rule, string action) =>
            rule != null && AutomationCatalog.IdComparer.Equals(rule.action?.Trim(), action);

        private static bool IsDefaultBlockingLevel(string level) =>
            AutomationCatalog.IdComparer.Equals(level, "Error")
            || AutomationCatalog.IdComparer.Equals(level, "Exception")
            || AutomationCatalog.IdComparer.Equals(level, "Assert");

        private static void AddFingerprint(
            Dictionary<string, AutomationLogFingerprintCount> counts,
            ConsoleCaptureRecord record,
            string fingerprint)
        {
            if (!counts.TryGetValue(fingerprint, out AutomationLogFingerprintCount item))
            {
                item = new AutomationLogFingerprintCount
                {
                    fingerprint = fingerprint,
                    level = record.logType,
                    firstSequence = record.sequence,
                    lastSequence = record.sequence,
                };
                counts.Add(fingerprint, item);
            }
            item.count++;
            item.lastSequence = record.sequence;
        }
    }
}
