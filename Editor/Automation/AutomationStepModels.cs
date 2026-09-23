using System;
using System.Collections.Generic;
using UnityEngine;

namespace CodingRiver.UPilot.Automation
{
    public enum AutomationStepStatus { Running, Succeeded, SucceededWithWarnings, Skipped, Failed, Canceled, TimedOut }
    public enum AutomationStepRestore { Unsupported, Restored }

    public sealed class AutomationStepException : Exception
    {
        public string Code { get; }
        public AutomationStepException(string code, string message) : base(message) { Code = code; }
    }

    [Serializable]
    public sealed class AutomationStepResult
    {
        public AutomationStepStatus status;
        public string errorCode = "";
        public static AutomationStepResult Result(AutomationStepStatus status, string code = "") =>
            new() { status = status, errorCode = code };
    }

    [Serializable]
    public sealed class AutomationStepError
    {
        public string code = "";
        public string message = "";
        public string diagnostic = "";
    }

    [Serializable]
    public sealed class AutomationStepValidation
    {
        public bool ok = true;
        public List<AutomationDiagnostic> diagnostics = new();
        public double budgetSeconds;
        public static AutomationStepValidation Invalid(string code, string message)
        {
            var result = new AutomationStepValidation { ok = false };
            AutomationCatalog.Add(result.diagnostics, code, message);
            return result;
        }
    }

    [Serializable]
    public sealed class AutomationStepItem
    {
        public string instanceId;
        public string stepId;
        public string arguments = "";
        public string phase = "Normal";
        public double timeoutSeconds = -1;
        public double pollIntervalSeconds = -1;
        public double cleanupTimeoutSeconds = 10;
        public bool allowSkipped;
    }

    [Serializable]
    public sealed class AutomationStepPlan
    {
        public int version = 1;
        public AutomationStepItem[] steps = Array.Empty<AutomationStepItem>();
        public AutomationLogRule[] logPolicy = Array.Empty<AutomationLogRule>();
    }

    [Serializable]
    public sealed class AutomationStepDescriptor
    {
        public string stepId;
        public string description;
        public string argumentsExample;
        public string typeIdentity;
        public double timeoutSeconds;
        public double pollIntervalSeconds;
    }

    [Serializable]
    public sealed class AutomationStepRecord
    {
        public string instanceId;
        public string typeIdentity;
        public string stage = "Pending";
        public AutomationStepStatus outcome;
        public AutomationStepError error;
        public string checkpointJson = "{}";
        public long startedAtUtcMs;
        public long cleanupStartedAtUtcMs;
        public long finishedAtUtcMs;
        public bool cancelSent;
    }

    [Serializable]
    public sealed class AutomationStepRun
    {
        public int version = 1;
        public string runId;
        public string operationId;
        public string editorIdentity;
        public string planHash;
        public AutomationStepPlan plan;
        public List<AutomationStepRecord> steps = new();
        public int cursor;
        public string stage = "Executing";
        public string status = "Running";
        public bool terminal;
        public bool cleanupPending;
        public bool recoveryRequired;
        public bool cancelRequested;
        public long startedAtUtcMs;
        public long finishedAtUtcMs;
        public AutomationStepError error;
        public List<AutomationStepError> secondaryErrors = new();
        public string sharedCheckpointJson = "{}";
        public string captureSessionId = "";
        public bool captureOwned;
        public bool captureStartIntent;
        public bool captureStopVerified;
        public long captureStartSequence;
        public long captureEndSequence = -1;
        public List<AutomationLogInterval> intervals = new();
        public long intervalStartSequence;
        public AutomationReportArtifact[] artifacts = Array.Empty<AutomationReportArtifact>();
        public List<AutomationReportArtifact> registeredArtifacts = new();
        public List<AutomationSnapshotRecord> snapshots = new();
        public AutomationReportLogSummary logSummary;
        public string reportCommitJson = "";
        public string reportDirectory;
    }

    internal static class AutomationStepJson
    {
        internal static T Copy<T>(T value) => JsonUtility.FromJson<T>(JsonUtility.ToJson(value));
        internal static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        internal static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
